from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class TermSetImportResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The created property
    created: Optional[bool] = None
    # The termSetId property
    term_set_id: Optional[UUID] = None
    # The termsCreated property
    terms_created: Optional[int] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> TermSetImportResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: TermSetImportResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return TermSetImportResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "created": lambda n : setattr(self, 'created', n.get_bool_value()),
            "termSetId": lambda n : setattr(self, 'term_set_id', n.get_uuid_value()),
            "termsCreated": lambda n : setattr(self, 'terms_created', n.get_int_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("created", self.created)
        writer.write_uuid_value("termSetId", self.term_set_id)
        writer.write_int_value("termsCreated", self.terms_created)
        writer.write_additional_data_value(self.additional_data)
    

