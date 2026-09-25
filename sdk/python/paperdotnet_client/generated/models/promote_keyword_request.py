from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class PromoteKeywordRequest(AdditionalDataHolder, Parsable):
    """
    Promotes a keyword into `termSetId` (under `parentId`, optional).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The parentId property
    parent_id: Optional[UUID] = None
    # The termSetId property
    term_set_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PromoteKeywordRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PromoteKeywordRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PromoteKeywordRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "parentId": lambda n : setattr(self, 'parent_id', n.get_uuid_value()),
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
        writer.write_uuid_value("parentId", self.parent_id)
        writer.write_uuid_value("termSetId", self.term_set_id)
        writer.write_additional_data_value(self.additional_data)
    

