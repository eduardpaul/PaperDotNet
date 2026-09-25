from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class PromoteKeywordResponse(AdditionalDataHolder, Parsable):
    """
    Result of a promotion: the term, and whether the keyword was merged into an existing term.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The merged property
    merged: Optional[bool] = None
    # The name property
    name: Optional[str] = None
    # The termId property
    term_id: Optional[UUID] = None
    # The termSetId property
    term_set_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PromoteKeywordResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PromoteKeywordResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PromoteKeywordResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "merged": lambda n : setattr(self, 'merged', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "termId": lambda n : setattr(self, 'term_id', n.get_uuid_value()),
            "termSetId": lambda n : setattr(self, 'term_set_id', n.get_uuid_value()),
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
        writer.write_bool_value("merged", self.merged)
        writer.write_str_value("name", self.name)
        writer.write_uuid_value("termId", self.term_id)
        writer.write_uuid_value("termSetId", self.term_set_id)
        writer.write_additional_data_value(self.additional_data)
    

