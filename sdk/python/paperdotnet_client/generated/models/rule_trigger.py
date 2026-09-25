from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class RuleTrigger(AdditionalDataHolder, Parsable):
    """
    When a rule runs: `type` is `itemAdded`, `itemUpdated`, `itemDeleted` or an extension trigger;`list` and `contentType` narrow it by name; `changedFields` (updates) needs one of them to change.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changedFields property
    changed_fields: Optional[list[str]] = None
    # The contentType property
    content_type: Optional[str] = None
    # The list property
    list_: Optional[str] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RuleTrigger:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RuleTrigger
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RuleTrigger()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "changedFields": lambda n : setattr(self, 'changed_fields', n.get_collection_of_primitive_values(str)),
            "contentType": lambda n : setattr(self, 'content_type', n.get_str_value()),
            "list": lambda n : setattr(self, 'list_', n.get_str_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
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
        writer.write_collection_of_primitive_values("changedFields", self.changed_fields)
        writer.write_str_value("contentType", self.content_type)
        writer.write_str_value("list", self.list_)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

